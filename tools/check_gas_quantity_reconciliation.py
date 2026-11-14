"""Explicit same-instant physical quantity amendments on disposable PostgreSQL."""
import json
from decimal import Decimal
from uuid import uuid4
from datetime import datetime, timezone

from check_reference_editing import main
from check_gas_physical_known import checks as known_checks, upgrade_checks as known_upgrade_checks, fixture
from check_gas_quantity_reconciliation_cases import checks as allocation_checks, upgrade_checks as quantity_upgrade_checks


def upgrade_checks(query, migration):
    known_upgrade_checks(query, migration)
    quantity_upgrade_checks(query, migration)


def checks(query, manifest):
    known_checks(query, manifest)
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    def literal(value):return "'"+json.dumps(value).replace("'","''")+"'::jsonb"
    def call(sql):
        token=query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{sql}COMMIT;")
    def capture():return call(f"SELECT security.capture_date_context('{catalog}','2026-11-06',NULL);")
    # Retire one receipt which has not funded the independent short. Its title
    # disappears and its acquisition obligation returns; actual bank payments
    # and the other receipts remain exactly as recorded.
    source=json.loads(query(f"SELECT to_jsonb(p)||jsonb_build_object('account_id',s.account_id,'source_key',e.source_key,'evidence_revision',e.revision) "
        "FROM trading.gas_futures_delivery_performances p JOIN trading.gas_owned_positions s ON(s.catalog_id,s.id)=(p.catalog_id,p.receipt_lot_id) "
        "JOIN trading.gas_owned_evidence e ON(e.catalog_id,e.id)=(p.catalog_id,p.title_evidence_id) "
        f"WHERE p.catalog_id='{catalog}' AND NOT EXISTS(SELECT 1 FROM trading.gas_owned_consumptions c WHERE(c.catalog_id,c.lot_id)=(p.catalog_id,p.receipt_lot_id)) "
        "ORDER BY p.performed_at DESC,p.business_reference LIMIT 1;"))
    assert source and Decimal(source['quantity'])==Decimal('.25')
    context=capture()
    current=json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','2026-11-06T16:00:00Z');"
        f"SELECT security.read_gas_futures_reconciliation('{catalog}','{source['position_id']}','{source['id']}','{context}','2026-11-06T16:00:00Z')::text;"))
    assert current['performance']['revision']==1 and current['performance']['id']==source['id']
    policy=str(uuid4())
    call(f"SELECT lifecycle.save_policy('{catalog}',{literal(dict(operationId=str(uuid4()),policyId=policy,expectedRevision=0,action='gas.futures.reconcile_quantity',mode='manual',fromAt='2026-10-01T00:00:00Z',untilAt='2027-01-01T00:00:00Z',implementation='explicit_lifecycle_action_policy_v1'))});")
    at='2026-11-06T16:00:00Z'
    call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','bind_policy','{source['account_id']}',"
        f"{literal(dict(action='gas.futures.reconcile_quantity',policyId=policy,policyRevision=1,effectiveAt='2026-10-01T00:00:00Z'))},'{at}','{capture()}',NULL,NULL,false);")
    occurred=datetime.fromisoformat(source['performed_at']).astimezone(timezone.utc).strftime('%Y-%m-%dT%H:%M:%S.%fZ')
    amended=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{source['account_id']}',"
        f"{literal(dict(sourceKey=source['source_key'],kind='title_receipt',quantity='0',occurredAt=occurred,evidence='Synthetic retracted title evidence',expectedRevision=source['evidence_revision']))},'{at}','{capture()}',NULL,NULL,false)::text;"))
    performances=json.loads(query(f"SELECT jsonb_agg(to_jsonb(p) ORDER BY p.performed_at,convert_to(p.business_reference,'UTF8')) FROM trading.gas_futures_delivery_performances p WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}');"))
    run=json.loads(query(f"SELECT to_jsonb(r) FROM trading.gas_futures_delivery_principal_runs r WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}') ORDER BY revision DESC LIMIT 1;"))
    previous=json.loads(query(f"SELECT jsonb_object_agg(performance_id,to_jsonb(d)) FROM trading.gas_futures_delivery_principal_determinations d WHERE(catalog_id,position_id,run_revision)=('{catalog}','{source['position_id']}',{run['revision']});"))
    link=json.loads(query(f"SELECT to_jsonb(l)||jsonb_build_object('source',d.source_observation_id) FROM settlement_data.futures_delivery_obligations l JOIN settlement_data.futures_margin_determinations d ON(d.catalog_id,d.id)=(l.catalog_id,'{run['final_determination_id']}') WHERE(l.catalog_id,l.position_id)=('{catalog}','{source['position_id']}');"))
    # Independent known-price oracle: each retained quarter at 6.03 has these
    # exact and rounded cumulative totals, including the explicit zero prefix.
    exact=['0','-1.5075','-3.015','-4.5225'];rounded=['0','-1.51','-3.02','-4.52']
    retained=0;prior_total=Decimal(0);steps=[]
    for performance in performances:
        retained+=performance['id']!=source['id']
        total=Decimal(rounded[retained]);contribution=total-prior_total;prior=previous[performance['id']]
        steps.append(dict(performanceId=performance['id'],previousDeterminationId=prior['id'],cumulativeQuantity=str(Decimal('.25')*retained),
            exactTotal=exact[retained],roundedTotal=str(total),contribution=str(contribution),adjustment=str(contribution-Decimal(str(prior['contribution'])))))
        prior_total=total
    assert retained==3 and sum(Decimal(s['adjustment']) for s in steps)==Decimal('1.51')
    continuation=dict(implementation='gas_futures_delivery_continuation_v1',positionId=source['position_id'],expectedVersion=run['revision'],finalOccurrenceId=link['final_occurrence_id'],sourceObservationId=link['source'],steps=steps)
    selection=dict(positionId=source['position_id'],performanceId=source['id'],expectedRevision=1,expectedPrincipalVersion=run['revision'],quantity='0',retracted=True,titleEvidenceId=amended['id'],supply=[],sourceAllocation='preserve_unallocated_source_quantity_v1',evidence='Synthetic observed title retraction')
    request=str(uuid4());selected_context=capture()
    payment_hash=query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")
    command=f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{request}','{source['position_id']}',{literal(selection)},'{at}','{selected_context}','{policy}',1,false,{literal(continuation)})::text;"
    accepted=json.loads(call(command))
    assert accepted['currentRevision']==2 and accepted['principalVersion']==run['revision']+1 and accepted['changed']
    assert json.loads(call(command))==accepted
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}');")=='-4.52'
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{source['receipt_lot_id']}');")=='0'
    assert query(f"SELECT md5(coalesce(jsonb_agg(to_jsonb(p) ORDER BY id),'[]')::text) FROM trading.gas_futures_delivery_payments p WHERE catalog_id='{catalog}';")==payment_hash
    retired_context=capture()
    def selected(context,instant=at):
        return json.loads(call(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{instant}');"
            f"SELECT security.read_gas_futures_reconciliation('{catalog}','{source['position_id']}','{source['id']}','{context}','{instant}')::text;"))['performance']
    assert selected(context)['revision']==1 and Decimal(str(selected(context)['quantity']))==Decimal('.25')
    assert selected(retired_context)['revision']==2 and Decimal(str(selected(retired_context)['quantity']))==0
    def record(identity,body,plan=None):
        return json.loads(call(f"SELECT security.reconcile_gas_futures_delivery('{catalog}','{identity}','{source['position_id']}',{literal(body)},'{at}','{capture()}','{policy}',1,false,{literal(plan) if plan is not None else 'NULL'})::text;"))
    before_ack=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    original_fact={**selection,'expectedRevision':2,'expectedPrincipalVersion':accepted['principalVersion'],'quantity':'0.25','retracted':False,'titleEvidenceId':source['title_evidence_id']}
    ack=record(str(uuid4()),original_fact)
    assert not ack['changed'] and ack['acknowledgedRevision']==1 and ack['currentRevision']==2
    assert query(f"SELECT quantity FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{source['receipt_lot_id']}');")=='0'
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before_ack
    # A new source document is an explicit reinstatement. Reusing the old
    # positive document above acknowledged history without rewinding state.
    restored_evidence=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{source['account_id']}',"
        f"{literal(dict(sourceKey=source['source_key'],kind='title_receipt',quantity='0.25',occurredAt=occurred,evidence='Synthetic source revision explicitly reinstating title',expectedRevision=2))},'{at}','{capture()}',NULL,NULL,false)::text;"))
    def plan_for(quantities):
        latest=json.loads(query(f"SELECT to_jsonb(r) FROM trading.gas_futures_delivery_principal_runs r WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}') ORDER BY revision DESC LIMIT 1;"))
        prior=json.loads(query(f"SELECT jsonb_object_agg(performance_id,to_jsonb(d)) FROM trading.gas_futures_delivery_principal_determinations d WHERE(catalog_id,position_id,run_revision)=('{catalog}','{source['position_id']}',{latest['revision']});"))
        totals=['0','-1.51','-3.02','-4.52','-6.03'];exact_totals=['0','-1.5075','-3.015','-4.5225','-6.03']
        count=0;previous_total=Decimal(0);values=[]
        for performance in performances:
            count+=quantities[performance['id']]
            total=Decimal(totals[count]);contribution=total-previous_total;previous=prior[performance['id']]
            values.append(dict(performanceId=performance['id'],previousDeterminationId=previous['id'],cumulativeQuantity=str(Decimal('.25')*count),exactTotal=exact_totals[count],
                roundedTotal=str(total),contribution=str(contribution),adjustment=str(contribution-Decimal(str(previous['contribution'])))))
            previous_total=total
        return {**continuation,'expectedVersion':latest['revision'],'steps':values}
    restored={**selection,'expectedRevision':2,'expectedPrincipalVersion':accepted['principalVersion'],'quantity':'0.25','retracted':False,'titleEvidenceId':restored_evidence['id']}
    restored_plan=plan_for({p['id']:1 for p in performances})
    restored_result=record(str(uuid4()),restored,restored_plan)
    assert restored_result['changed'] and restored_result['currentRevision']==3
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}');")=='-6.03'
    assert selected(retired_context)['revision']==2 and Decimal(str(selected(retired_context)['quantity']))==0
    future_context=call(f"SELECT security.capture_date_context('{catalog}','2026-11-04',NULL);")
    future=selected(future_context,'2026-11-04T08:00:00Z')
    assert future['revision']==3 and Decimal(str(future['quantity']))==Decimal('.25') and future['performed_at']>'2026-11-04T08:00:00'
    before_ack=query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")
    ack=record(str(uuid4()),{**selection,'expectedRevision':3,'expectedPrincipalVersion':restored_result['principalVersion']})
    assert not ack['changed'] and ack['acknowledgedRevision']==2 and ack['currentRevision']==3
    assert query(f"SELECT trim_scale(quantity) FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{source['receipt_lot_id']}');")=='0.25'
    assert query(f"SELECT max(publication_order) FROM trading.publications WHERE catalog_id='{catalog}';")==before_ack
    managed_evidence=json.loads(call(f"SELECT security.record_gas_owned('{catalog}','{uuid4()}','evidence','{source['account_id']}',"
        f"{literal(dict(sourceKey=source['source_key'],kind='title_receipt',quantity='0.125',occurredAt=occurred,evidence='Synthetic source revision for reviewed partial title',expectedRevision=3))},'{at}','{capture()}',NULL,NULL,false)::text;"))
    from pathlib import Path
    import os
    import subprocess
    host=query('SHOW unix_socket_directories;');packet=Path(host)/'quantity-reconciliation-check.json'
    packet.write_text(json.dumps(dict(manifest=manifest,host=host,contextId=capture(),at=at,positionId=source['position_id'],performanceId=source['id'],
        revision=3,principalVersion=restored_result['principalVersion'],evidenceId=managed_evidence['id'],policyId=policy)))
    packet.chmod(0o600)
    managed=subprocess.run([os.environ.get('DOTNET','dotnet'),'run','--no-build','--no-restore','--project','tests/OpenEtrm.Api.Checks','--','--gas-quantity-reconciliation',str(packet)],
        cwd=Path(__file__).resolve().parents[1],capture_output=True,text=True,timeout=60)
    if managed.returncode:raise RuntimeError(managed.stdout+managed.stderr)
    print(managed.stdout.strip(),flush=True)
    assert query(f"SELECT trim_scale(quantity) FROM trading.gas_owned_positions_current WHERE(catalog_id,id)=('{catalog}','{source['receipt_lot_id']}');")=='0.125'
    assert query(f"SELECT trim_scale(sum(amount)) FROM trading.gas_futures_delivery_cash WHERE(catalog_id,position_id)=('{catalog}','{source['position_id']}');")=='-5.28'
    print('PASS quantity reconciliation: explicit title retirement, complete principal refund prefix, stable native identity, unchanged bank payments and first outcome',flush=True)
    print('PASS financial head source: old cutoffs retain old revisions, known future effective heads survive, explicit reinstatement and historical acknowledgements never rewind current state',flush=True)
    allocation_checks(query,manifest)


if __name__=='__main__':main(checks=checks,fixture_factory=fixture,before_migration=upgrade_checks)
